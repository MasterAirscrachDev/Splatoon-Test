using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InkEmitter : MonoBehaviour
{
    [SerializeField] int splashSize = 1, team;
    [SerializeField] bool SingleUse = true, hashit = false;
    [SerializeField] List<Transform> hitSurfaces = new List<Transform>();
    RaycastHit hitT;
    Ray ray;
    public void Init(int splashSize, int team){
        this.splashSize = splashSize;
        this.team = team;
    }

    // Update is called once per frame
    void Update()
    {
        //raycast down
        ray = new Ray(transform.position, Vector3.down);
        RaycastSurface(ray);
        //raycast forward
        ray = new Ray(transform.position, Vector3.forward);
        RaycastSurface(ray);
        //raycast right
        ray = new Ray(transform.position, Vector3.right);
        RaycastSurface(ray);
        //raycast left
        ray = new Ray(transform.position, Vector3.left);
        RaycastSurface(ray);
        //raycast up
        ray = new Ray(transform.position, Vector3.up);
        RaycastSurface(ray);
        //raycast back
        ray = new Ray(transform.position, Vector3.back);
        RaycastSurface(ray);
        if(hashit){
            if(gameObject.GetComponentInParent<ProjectileSystem>() != null){
                gameObject.GetComponentInParent<ProjectileSystem>().DeleteProjectile();
            }
        }
    }
    void RaycastSurface(Ray ray){
        Debug.DrawRay(ray.origin, ray.direction * 0.1f, Color.red);
        if(Physics.Raycast(ray, out hitT, 0.1f)){
            if(SingleUse){
                if(!hitSurfaces.Contains(hitT.transform)){
                    hitSurfaces.Add(hitT.transform);
                    InkSpawn(hitT);
                }
            }
            else{ InkSpawn(hitT); }
            hashit = true;
        }
    }
    void InkSpawn(RaycastHit hit){
        if(hit.transform.GetComponent<SurfaceInkManager>() == null){ return; }
        hit.transform.GetComponent<SurfaceInkManager>().Splat(hit.textureCoord, splashSize, team);
    }
}
